using Godot;
using System;

public partial class CoordinateGrid : Node2D
{
    // region ПРИВЯЗКИ (Настраиваются в инспекторе)
    [ExportGroup("Привязки")]
    [Export] private Control _targetBackground; // Ссылка на панель (BlackPanel), поверх которой рисуем
    [Export] private FontFile _font;            // Шрифт для подписей
    // endregion

    // region НАСТРОЙКИ СЕТКИ (В МИЛЛИМЕТРАХ)
    [ExportGroup("Реальные размеры (мм)")]
    [Export] public float RealWorldWidthMM { get; set; } = 1600f; // 1600 мм (было 160 см)
    [Export] public float RealWorldHeightMM { get; set; } = 900f; // 900 мм (было 90 см)

    [ExportGroup("Стиль линий")]
    [Export] public Color MajorLineColor { get; set; } = new Color(0.1f, 0.2f, 0.5f, 0.9f);
    [Export] public Color MinorLineColor { get; set; } = new Color(0.2f, 0.2f, 0.3f, 0.6f);
    [Export] public int MajorLineWidth { get; set; } = 2;
    [Export] public int MinorLineWidth { get; set; } = 1;
    // endregion

    private float[] _points = new float[3];
    private Color[] _pointColors = new Color[3];

    /// <summary>
    /// Динамически вычисляемая область отрисовки.
    /// Берет актуальные размеры и позицию целевой панели.
    /// </summary>
    public Rect2 GridArea => _targetBackground != null
        ? _targetBackground.GetRect()
        : new Rect2(0, 0, 100, 100);

    /// <summary>
    /// Пикселей на миллиметр по горизонтали
    /// </summary>
    public float PixelsPerMM_X => GridArea.Size.X / Math.Max(1, RealWorldWidthMM);

    /// <summary>
    /// Пикселей на миллиметр по вертикали
    /// </summary>
    public float PixelsPerMM_Y => GridArea.Size.Y / Math.Max(1, RealWorldHeightMM);


    // region ИНИЦИАЛИЗАЦИЯ
    public override void _Ready()
    {
        ZIndex = 2; // Рисуем поверх фона

        // 1. Попытка найти зависимости
        if (_targetBackground == null)
        {
            var node = GetNodeOrNull<Control>("../BlackPanel");
            if (node != null)
            {
                _targetBackground = node;
            }
            else
            {
                GD.PrintErr("CoordinateGrid: ОШИБКА! Не указан _targetBackground (BlackPanel) в инспекторе!");
            }
        }

        if (_font == null)
        {
            try { _font = GD.Load<FontFile>("res://fonts/times.ttf"); } catch { }
        }

        // 2. Подписка на изменение размеров панели
        if (_targetBackground != null)
        {
            _targetBackground.Resized += QueueRedraw;
        }
        else
        {
            GetViewport().SizeChanged += QueueRedraw;
        }
    }
    // endregion


    // region ПУБЛИЧНЫЕ МЕТОДЫ
    public void UpdatePoints(float[] positions, Color[] colors)
    {
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
        if (_font == null || _targetBackground == null) return;
        if (GridArea.Size.X <= 1 || GridArea.Size.Y <= 1) return;

        DrawVerticalLines();
        DrawHorizontalLines();
        DrawPointMarkers();
    }

    private void DrawVerticalLines()
    {
        // Шаг 50 мм (5 см) - вспомогательные линии
        // Шаг 100 мм (10 см) - основные линии
        for (float mm = 0; mm <= RealWorldWidthMM; mm += 50)
        {
            // Основная линия каждые 100 мм
            bool isMajorLine = Mathf.IsEqualApprox(mm % 100, 0);

            float x = GridArea.Position.X + mm * PixelsPerMM_X;

            DrawLine(
                new Vector2(x, GridArea.Position.Y),
                new Vector2(x, GridArea.End.Y),
                isMajorLine ? MajorLineColor : MinorLineColor,
                isMajorLine ? MajorLineWidth : MinorLineWidth
            );

            // Подписи только для основных линий (кроме 0)
            if (isMajorLine && mm > 0)
            {
                // F0 - формат без знаков после запятой (100, 200, 300)
                string label = $"{mm:F0}";
                Vector2 textPos = new Vector2(x + 2, GridArea.End.Y - 5);
                DrawString(_font, textPos, label, fontSize: 14, modulate: Colors.White);
            }
        }
    }

    private void DrawHorizontalLines()
    {
        // Шаг 50 мм (5 см)
        for (float mm = 0; mm <= RealWorldHeightMM; mm += 50)
        {
            // Основная линия каждые 100 мм
            bool isMajorLine = Mathf.IsEqualApprox(mm % 100, 0);

            // Инверсия Y (0 внизу)
            float invertedMM = RealWorldHeightMM - mm;
            float y = GridArea.Position.Y + mm * PixelsPerMM_Y;

            DrawLine(
                new Vector2(GridArea.Position.X, y),
                new Vector2(GridArea.End.X, y),
                isMajorLine ? MajorLineColor : MinorLineColor,
                isMajorLine ? MajorLineWidth : MinorLineWidth
            );

            if (isMajorLine)
            {
                string label = $"{invertedMM:F0}";
                Vector2 textPos = new Vector2(GridArea.Position.X + 5, y - 2);
                DrawString(_font, textPos, label, fontSize: 14, modulate: Colors.White);
            }
        }
    }

    private void DrawPointMarkers()
    {
        if (_points == null) return;

        for (int i = 0; i < 3; i++)
        {
            if (_points[i] <= 0) continue;

            // _points[i] теперь считается в ММ
            float x = GridArea.Position.X + _points[i] * PixelsPerMM_X;
            Color color = _pointColors[i];

            // Линия
            DrawLine(
                new Vector2(x, GridArea.Position.Y),
                new Vector2(x, GridArea.End.Y),
                color,
                3
            );

            // Метка "Точка N"
            DrawString(
                _font,
                new Vector2(x + 5, GridArea.Position.Y + 20),
                $"Точка {i}",
                fontSize: 16,
                modulate: color
            );

            // Метка "XXX мм"
            DrawString(
                _font,
                new Vector2(x + 5, GridArea.Position.Y + 40),
                $"{_points[i]:F1} мм", // F1 оставит 1 знак (например 100.5 мм), если нужно точно
                fontSize: 14,
                modulate: color
            );
        }
    }
    // endregion
}