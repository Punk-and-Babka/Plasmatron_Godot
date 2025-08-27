using Godot;
using System;

public partial class CoordinateGrid : Node2D
{
    // region Настройки сетки
    [ExportGroup("Реальные размеры")]
    [Export] public float RealWorldWidthCM { get; set; } = 160f; // Реальная ширина области в см
    [Export] public float RealWorldHeightCM { get; set; } = 90f; // Реальная высота области в см

    [ExportGroup("Стиль линий")]
    [Export] public Color MajorLineColor { get; set; } = new Color(0.1f, 0.2f, 0.5f, 0.9f);  // Цвет основных линий
    [Export] public Color MinorLineColor { get; set; } = new Color(0.2f, 0.2f, 0.3f, 0.6f);   // Цвет вспомогательных линий
    [Export] public int MajorLineWidth { get; set; } = 2;  // Толщина основных линий
    [Export] public int MinorLineWidth { get; set; } = 1;  // Толщина вспомогательных линий

    private float[] _points = new float[3];
    private Color[] _pointColors = new Color[3];

    public void UpdatePoints(float[] positions, Color[] colors)
    {
        Array.Copy(positions, _points, 3);
        Array.Copy(colors, _pointColors, 3);
        QueueRedraw();
    }

    /// <summary>
    /// Область отрисовки сетки (позиция и размер в пикселях)
    /// </summary>
    public Rect2 GridArea { get; set; }

    /// <summary>
    /// Пикселей на сантиметр по горизонтали
    /// </summary>
    public float PixelsPerCM_X => GridArea.Size.X / RealWorldWidthCM;

    /// <summary>
    /// Пикселей на сантиметр по вертикали
    /// </summary>
    public float PixelsPerCM_Y => GridArea.Size.Y / RealWorldHeightCM;
    // endregion

    private FontFile _font; // Шрифт для подписей

    private void DrawPointMarkers()
    {
        if (_points == null) return;

        for (int i = 0; i < 3; i++)
        {
            if (_points[i] <= 0) continue;

            float x = GridArea.Position.X + _points[i] * PixelsPerCM_X;
            Color color = _pointColors[i];

            // Вертикальная линия
            DrawLine(
                new Vector2(x, GridArea.Position.Y),
                new Vector2(x, GridArea.Position.Y + GridArea.Size.Y),
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
                new Vector2(x + 5, GridArea.Position.Y + 40), // +20 пикселей по Y
                $"{_points[i]:N1} см",
                fontSize: 14,
                modulate: color
            );
        }
    }

    // region Инициализация
    public override void _Ready()
    {
        // Получаем размеры целевого прямоугольника (черная панель)
        var blackPanel = GetNode<ColorRect>("../BlackPanel");
        GridArea = new Rect2(blackPanel.Position, blackPanel.Size);

        // Настройка порядка отрисовки и обработки изменений
        ZIndex = 2; // Рисуем поверх фона
        GetViewport().SizeChanged += () => QueueRedraw(); // Автообновление при изменении размеров

        // Загрузка шрифта из файла
        _font = GD.Load<FontFile>("res://fonts/times.ttf");
    }
    // endregion

    // region Обновление сетки
    /// <summary>
    /// Обновить размеры области отрисовки
    /// </summary>
    public void UpdateGridSize(Vector2 newSize)
    {
        GridArea = new Rect2(GridArea.Position, newSize);
        QueueRedraw();
    }

    /// <summary>
    /// Событие обновления сетки
    /// </summary>
    public event Action GridUpdated;

    /// <summary>
    /// Принудительное обновление сетки
    /// </summary>
    public void UpdateGrid()
    {
        GridUpdated?.Invoke();
        QueueRedraw();
    }
    // endregion

    // region Отрисовка
    private Vector2 _lastGridSize;
    public override void _Draw()
    {
        if (_font == null || GridArea.Size == Vector2.Zero) return;
        _lastGridSize = GridArea.Size;

        DrawVerticalLines(); // Отрисовка вертикальных линий
        DrawHorizontalLines(); // Отрисовка горизонтальных линий
        DrawPointMarkers();
    }

    /// <summary>
    /// Отрисовка вертикальных линий и подписей
    /// </summary>
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
                new Vector2(x, GridArea.Position.Y + GridArea.Size.Y),
                isMajorLine ? MajorLineColor : MinorLineColor,
                isMajorLine ? MajorLineWidth : MinorLineWidth
            );

            // Подписи для основных линий (кроме нулевой)
            if (isMajorLine && cm > 0)
            {
                string label = $"{cm} см";
                Vector2 textPos = new Vector2(
                    x + 2, // Небольшой отступ от линии
                    GridArea.Position.Y + GridArea.Size.Y - 10 // Выравнивание по нижнему краю
                );
                DrawString(_font, textPos, label, fontSize: 14, modulate: Colors.White);
            }
        }
    }

    /// <summary>
    /// Отрисовка горизонтальных линий и подписей
    /// </summary>
    private void DrawHorizontalLines()
    {
        // Шаг 5 см (основные линии через 10 см)
        for (float cm = 0; cm <= RealWorldHeightCM; cm += 5)
        {
            bool isMajorLine = Mathf.IsEqualApprox(cm % 10, 0);
            float invertedCM = RealWorldHeightCM - cm; // Инверсия для отсчета снизу
            float y = GridArea.Position.Y + cm * PixelsPerCM_Y;

            // Рисуем линию
            DrawLine(
                new Vector2(GridArea.Position.X, y),
                new Vector2(GridArea.Position.X + GridArea.Size.X, y),
                isMajorLine ? MajorLineColor : MinorLineColor,
                isMajorLine ? MajorLineWidth : MinorLineWidth
            );

            // Подписи для основных линий
            if (isMajorLine)
            {
                string label = $"{invertedCM} см"; // Используем инвертированное значение
                Vector2 textPos = new Vector2(
                    GridArea.Position.X + 5, // Отступ от левого края
                    y - 2   // Выравнивание по центру линии
                );
                DrawString(_font, textPos, label, fontSize: 14, modulate: Colors.White);
            }
        }
    }
    // endregion
}