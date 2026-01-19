using Godot;
using System;

public partial class CoordinateGrid : Node2D
{
    // region ПРИВЯЗКИ
    [ExportGroup("Привязки")]
    [Export] private Control _targetBackground;
    [Export] private FontFile _font;
    // endregion

    // region НАСТРОЙКИ СЕТКИ (ММ)
    [ExportGroup("Реальные размеры (мм)")]
    [Export] public float RealWorldWidthMM { get; set; } = 1600f;
    [Export] public float RealWorldHeightMM { get; set; } = 900f;

    [ExportGroup("Стиль линий")]
    [Export] public Color MajorLineColor { get; set; } = new Color(0.1f, 0.2f, 0.5f, 0.9f);
    [Export] public Color MinorLineColor { get; set; } = new Color(0.2f, 0.2f, 0.3f, 0.6f);
    [Export] public int MajorLineWidth { get; set; } = 2;
    [Export] public int MinorLineWidth { get; set; } = 1;
    // endregion

    // ИЗМЕНЕНИЕ: Храним Vector2 вместо float
    private Vector2[] _points = new Vector2[3];
    private Color[] _pointColors = new Color[3];

    public Rect2 GridArea => _targetBackground != null
        ? _targetBackground.GetRect()
        : new Rect2(0, 0, 100, 100);

    public float PixelsPerMM_X => GridArea.Size.X / Math.Max(1, RealWorldWidthMM);
    public float PixelsPerMM_Y => GridArea.Size.Y / Math.Max(1, RealWorldHeightMM);

    public override void _Ready()
    {
        ZIndex = 2;
        if (_targetBackground == null) _targetBackground = GetNodeOrNull<Control>("../BlackPanel");
        if (_font == null) try { _font = GD.Load<FontFile>("res://fonts/times.ttf"); } catch { }

        if (_targetBackground != null) _targetBackground.Resized += QueueRedraw;
        else GetViewport().SizeChanged += QueueRedraw;
    }

    // ИЗМЕНЕНИЕ: Метод теперь принимает Vector2[]
    public void UpdatePoints(Vector2[] positions, Color[] colors)
    {
        int len = Math.Min(positions.Length, 3);
        Array.Copy(positions, _points, len);

        if (colors != null && colors.Length >= len)
            Array.Copy(colors, _pointColors, len);

        QueueRedraw();
    }

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
        for (float mm = 0; mm <= RealWorldWidthMM; mm += 50)
        {
            bool isMajorLine = Mathf.IsEqualApprox(mm % 100, 0);
            float x = GridArea.Position.X + mm * PixelsPerMM_X;

            DrawLine(new Vector2(x, GridArea.Position.Y), new Vector2(x, GridArea.End.Y),
                isMajorLine ? MajorLineColor : MinorLineColor, isMajorLine ? MajorLineWidth : MinorLineWidth);

            if (isMajorLine && mm > 0)
            {
                string label = $"{mm:F0} мм";
                DrawString(_font, new Vector2(x + 2, GridArea.End.Y - 5), label, fontSize: 14, modulate: Colors.White);
            }
        }
    }

    private void DrawHorizontalLines()
    {
        for (float mm = 0; mm <= RealWorldHeightMM; mm += 50)
        {
            bool isMajorLine = Mathf.IsEqualApprox(mm % 100, 0);
            float invertedMM = RealWorldHeightMM - mm;
            float y = GridArea.Position.Y + mm * PixelsPerMM_Y;

            DrawLine(new Vector2(GridArea.Position.X, y), new Vector2(GridArea.End.X, y),
                isMajorLine ? MajorLineColor : MinorLineColor, isMajorLine ? MajorLineWidth : MinorLineWidth);

            if (isMajorLine)
            {
                string label = $"{invertedMM:F0} мм";
                DrawString(_font, new Vector2(GridArea.Position.X + 5, y - 2), label, fontSize: 14, modulate: Colors.White);
            }
        }
    }

    private void DrawPointMarkers()
    {
        if (_points == null) return;

        for (int i = 0; i < 3; i++)
        {
            // Пропускаем, если точка (0,0) или отрицательная (не задана)
            if (_points[i].X <= 0 && _points[i].Y <= 0) continue;

            // Расчет позиции с учетом X и Y
            float x = GridArea.Position.X + _points[i].X * PixelsPerMM_X;
            // Y инвертируем (0 внизу)
            float yReal = RealWorldHeightMM - _points[i].Y;
            float y = GridArea.Position.Y + yReal * PixelsPerMM_Y;

            Color color = _pointColors[i];

            // Рисуем перекрестие вместо просто линии
            DrawLine(new Vector2(x, GridArea.Position.Y), new Vector2(x, GridArea.End.Y), color, 2); // Вертикаль
            DrawLine(new Vector2(GridArea.Position.X, y), new Vector2(GridArea.End.X, y), color, 2); // Горизонталь

            DrawString(_font, new Vector2(x + 5, y - 20), $"Точка {i}", fontSize: 16, modulate: color);
            DrawString(_font, new Vector2(x + 5, y), $"{_points[i].X:F0} : {_points[i].Y:F0}", fontSize: 14, modulate: color);
        }
    }
}