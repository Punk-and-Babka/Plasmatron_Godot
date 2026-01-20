using Godot;
using System;
using System.Collections.Generic;

public partial class CoordinateGrid : Node2D
{
    // region ПРИВЯЗКИ
    [ExportGroup("Привязки")]
    [Export] private Control _targetBackground;
    // endregion

    [ExportGroup("Реальные размеры (мм)")]
    [Export] public float RealWorldWidthMM { get; set; } = 1600f;
    [Export] public float RealWorldHeightMM { get; set; } = 900f;

    [ExportGroup("Стиль")]
    [Export] public Color MajorLineColor { get; set; } = new Color(0.2f, 0.4f, 0.8f, 0.5f);
    [Export] public Color MinorLineColor { get; set; } = new Color(0.2f, 0.4f, 0.8f, 0.2f);

    private List<Vector2> _points = new List<Vector2>();
    private List<Color> _pointColors = new List<Color>();
    private FontFile _font;

    public Rect2 GridArea => GetCurrentArea();

    public float PixelsPerMM_X => GridArea.Size.X / Math.Max(1, RealWorldWidthMM);
    public float PixelsPerMM_Y => GridArea.Size.Y / Math.Max(1, RealWorldHeightMM);

    public override void _Ready()
    {
        ZIndex = 1;

        if (_targetBackground == null)
        {
            _targetBackground = GetParent().GetNodeOrNull<Control>("BlackPanel");
            if (_targetBackground == null && GetParent().GetParent() != null)
            {
                _targetBackground = GetParent().GetParent().GetNodeOrNull<Control>("BlackPanel");
            }
        }

        if (_targetBackground == null)
        {
            GD.PrintErr("[CoordinateGrid] BlackPanel не найден! Проверь привязку.");
        }
        else
        {
            _targetBackground.Resized += QueueRedraw;
        }

        try { _font = GD.Load<FontFile>("res://fonts/times.ttf"); } catch { }

        CallDeferred(CanvasItem.MethodName.QueueRedraw);
    }

    private Rect2 GetCurrentArea()
    {
        if (_targetBackground != null && _targetBackground.IsInsideTree())
        {
            return new Rect2(Vector2.Zero, _targetBackground.Size);
        }
        return new Rect2(0, 0, RealWorldWidthMM, RealWorldHeightMM);
    }

    public void UpdatePoints(IEnumerable<Vector2> positions, IEnumerable<Color> colors)
    {
        _points.Clear();
        _pointColors.Clear();

        if (positions != null) _points.AddRange(positions);
        if (colors != null) _pointColors.AddRange(colors);

        while (_pointColors.Count < _points.Count) _pointColors.Add(Colors.White);

        QueueRedraw();
    }

    public override void _Draw()
    {
        Rect2 area = GridArea;
        if (area.Size.X < 10 || area.Size.Y < 10) return;

        DrawGridLines(area);
        DrawPointMarkers(area);
    }

    private void DrawGridLines(Rect2 area)
    {
        float scaleX = PixelsPerMM_X;
        float scaleY = PixelsPerMM_Y;

        // Вертикальные
        for (float mm = 0; mm <= RealWorldWidthMM; mm += 50)
        {
            bool major = Mathf.IsEqualApprox(mm % 100, 0);
            float x = area.Position.X + mm * scaleX;
            if (x > area.End.X) break;

            DrawLine(new Vector2(x, area.Position.Y), new Vector2(x, area.End.Y),
                major ? MajorLineColor : MinorLineColor, major ? 2 : 1);

            // ИЗМЕНЕНИЕ: Добавлено условие 'mm > 0', чтобы не рисовать 0 в начале
            if (major && _font != null && mm > 0)
                DrawString(_font, new Vector2(x + 2, area.End.Y - 5), $"{mm:F0}", fontSize: 16);
        }

        // Горизонтальные
        for (float mm = 0; mm <= RealWorldHeightMM; mm += 50)
        {
            bool major = Mathf.IsEqualApprox(mm % 100, 0);
            float invertedMM = RealWorldHeightMM - mm;
            float y = area.Position.Y + mm * scaleY;
            if (y > area.End.Y) break;

            DrawLine(new Vector2(area.Position.X, y), new Vector2(area.End.X, y),
                major ? MajorLineColor : MinorLineColor, major ? 2 : 1);

            // ИЗМЕНЕНИЕ: Добавлено условие 'invertedMM > 0', чтобы не рисовать 0 внизу
            if (major && _font != null && invertedMM > 0)
                DrawString(_font, new Vector2(area.Position.X + 5, y - 2), $"{invertedMM:F0}", fontSize: 16);
        }
    }

    private void DrawPointMarkers(Rect2 area)
    {
        if (_points == null) return;
        float scaleX = PixelsPerMM_X;
        float scaleY = PixelsPerMM_Y;

        for (int i = 0; i < _points.Count; i++)
        {
            float x = area.Position.X + _points[i].X * scaleX;
            float yReal = RealWorldHeightMM - _points[i].Y;
            float y = area.Position.Y + yReal * scaleY;
            Color c = _pointColors[i];

            DrawLine(new Vector2(x, area.Position.Y), new Vector2(x, area.End.Y), c, 1);
            DrawLine(new Vector2(area.Position.X, y), new Vector2(area.End.X, y), c, 1);
            DrawCircle(new Vector2(x, y), 4f, c);

            if (_font != null)
            {
                string label = $"{_points[i].X:0},{_points[i].Y:0}";
                // ИЗМЕНЕНИЕ: Увеличен шрифт с 16 до 24
                DrawString(_font, new Vector2(x + 5, y - 5), label, fontSize: 24, modulate: c);
            }
        }
    }
}