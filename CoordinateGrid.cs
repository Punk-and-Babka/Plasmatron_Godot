using Godot;
using System;

public partial class CoordinateGrid : Node2D
{
    // region ПРИВЯЗКИ (Настраиваются в инспекторе)
    [ExportGroup("Привязки")]
    [Export] private Control _targetBackground; // Ссылка на панель (BlackPanel), поверх которой рисуем
    [Export] private FontFile _font;            // Шрифт для подписей
    // endregion

    // region НАСТРОЙКИ СЕТКИ
    [ExportGroup("Реальные размеры")]
    [Export] public float RealWorldWidthCM { get; set; } = 160f; // Реальная ширина области в см
    [Export] public float RealWorldHeightCM { get; set; } = 90f; // Реальная высота области в см

    [ExportGroup("Стиль линий")]
    [Export] public Color MajorLineColor { get; set; } = new Color(0.1f, 0.2f, 0.5f, 0.9f);  // Цвет основных линий
    [Export] public Color MinorLineColor { get; set; } = new Color(0.2f, 0.2f, 0.3f, 0.6f);   // Цвет вспомогательных линий
    [Export] public int MajorLineWidth { get; set; } = 2;  // Толщина основных линий
    [Export] public int MinorLineWidth { get; set; } = 1;  // Толщина вспомогательных линий
    // endregion

    private float[] _points = new float[3];
    private Color[] _pointColors = new Color[3];

    /// <summary>
    /// Динамически вычисляемая область отрисовки.
    /// Берет актуальные размеры и позицию целевой панели.
    /// </summary>
    public Rect2 GridArea => _targetBackground != null
        ? _targetBackground.GetRect()
        : new Rect2(0, 0, 100, 100); // Заглушка, чтобы не делить на 0

    /// <summary>
    /// Пикселей на сантиметр по горизонтали
    /// </summary>
    public float PixelsPerCM_X => GridArea.Size.X / Math.Max(1, RealWorldWidthCM);

    /// <summary>
    /// Пикселей на сантиметр по вертикали
    /// </summary>
    public float PixelsPerCM_Y => GridArea.Size.Y / Math.Max(1, RealWorldHeightCM);


    // region ИНИЦИАЛИЗАЦИЯ
    public override void _Ready()
    {
        ZIndex = 2; // Рисуем поверх фона

        // 1. Попытка найти зависимости, если не заданы в инспекторе
        if (_targetBackground == null)
        {
            var node = GetNodeOrNull<Control>("../BlackPanel");
            if (node != null)
            {
                _targetBackground = node;
                GD.Print("CoordinateGrid: BlackPanel найден автоматически.");
            }
            else
            {
                GD.PrintErr("CoordinateGrid: ОШИБКА! Не указан _targetBackground (BlackPanel) в инспекторе!");
            }
        }

        if (_font == null)
        {
            try
            {
                _font = GD.Load<FontFile>("res://fonts/times.ttf");
            }
            catch
            {
                GD.PrintErr("CoordinateGrid: Шрифт не найден. Текст не будет отображаться.");
            }
        }

        // 2. Подписка на изменение размеров
        if (_targetBackground != null)
        {
            // Подписываемся именно на ресайз панели - это надежнее всего
            _targetBackground.Resized += OnContainerResized;
        }
        else
        {
            // Фоллбэк на ресайз окна
            GetViewport().SizeChanged += OnContainerResized;
        }
    }

    private void OnContainerResized()
    {
        QueueRedraw();
    }
    // endregion


    // region ПУБЛИЧНЫЕ МЕТОДЫ
    public void UpdatePoints(float[] positions, Color[] colors)
    {
        // Безопасное копирование данных
        int len = Math.Min(positions.Length, 3);
        Array.Copy(positions, _points, len);

        if (colors != null && colors.Length >= len)
        {
            Array.Copy(colors, _pointColors, len);
        }

        QueueRedraw();
    }

    public void UpdateGrid()
    {
        QueueRedraw();
    }
    // endregion


    // region ОТРИСОВКА
    public override void _Draw()
    {
        // Если зависимости не настроены, рисовать нельзя
        if (_font == null || _targetBackground == null) return;
        if (GridArea.Size.X <= 1 || GridArea.Size.Y <= 1) return;

        DrawVerticalLines();
        DrawHorizontalLines();
        DrawPointMarkers();
    }

    private void DrawVerticalLines()
    {
        // Шаг 5 см (основные линии через 10 см)
        for (float cm = 0; cm <= RealWorldWidthCM; cm += 5)
        {
            bool isMajorLine = Mathf.IsEqualApprox(cm % 10, 0);
            float x = GridArea.Position.X + cm * PixelsPerCM_X;

            // Рисуем линию
            DrawLine(
                new Vector2(x, GridArea.Position.Y),
                new Vector2(x, GridArea.End.Y), // Используем GridArea.End.Y
                isMajorLine ? MajorLineColor : MinorLineColor,
                isMajorLine ? MajorLineWidth : MinorLineWidth
            );

            // Подписи для основных линий (кроме нулевой)
            if (isMajorLine && cm > 0)
            {
                string label = $"{cm} см";
                // Позиционируем текст внизу области
                Vector2 textPos = new Vector2(
                    x + 2,
                    GridArea.End.Y - 5
                );
                DrawString(_font, textPos, label, fontSize: 14, modulate: Colors.White);
            }
        }
    }

    private void DrawHorizontalLines()
    {
        // Шаг 5 см (основные линии через 10 см)
        for (float cm = 0; cm <= RealWorldHeightCM; cm += 5)
        {
            bool isMajorLine = Mathf.IsEqualApprox(cm % 10, 0);

            // Инверсия для отсчета снизу (0 внизу)
            float invertedCM = RealWorldHeightCM - cm;

            float y = GridArea.Position.Y + cm * PixelsPerCM_Y;

            // Рисуем линию
            DrawLine(
                new Vector2(GridArea.Position.X, y),
                new Vector2(GridArea.End.X, y), // Используем GridArea.End.X
                isMajorLine ? MajorLineColor : MinorLineColor,
                isMajorLine ? MajorLineWidth : MinorLineWidth
            );

            // Подписи для основных линий
            if (isMajorLine)
            {
                string label = $"{invertedCM} см";
                Vector2 textPos = new Vector2(
                    GridArea.Position.X + 5,
                    y - 2
                );
                DrawString(_font, textPos, label, fontSize: 14, modulate: Colors.White);
            }
        }
    }

    private void DrawPointMarkers()
    {
        if (_points == null) return;

        for (int i = 0; i < 3; i++)
        {
            // Если точка <= 0, считаем её неактивной (или началом координат)
            // Если нужно рисовать точку 0, убери это условие
            if (_points[i] <= 0) continue;

            float x = GridArea.Position.X + _points[i] * PixelsPerCM_X;
            Color color = _pointColors[i];

            // Вертикальная линия маркера
            DrawLine(
                new Vector2(x, GridArea.Position.Y),
                new Vector2(x, GridArea.End.Y),
                color,
                3
            );

            // Название точки
            DrawString(
                _font,
                new Vector2(x + 5, GridArea.Position.Y + 20),
                $"Точка {i}",
                fontSize: 16,
                modulate: color
            );

            // Позиция
            DrawString(
                _font,
                new Vector2(x + 5, GridArea.Position.Y + 40),
                $"{_points[i]:N1} см",
                fontSize: 14,
                modulate: color
            );
        }
    }
    // endregion
}